using Godot;

namespace I76.Game.Editor;

/// <summary>
/// I76_SWITCH_TIMING=1: stamps the stages of a switch between the map editor and its playtest with the wall-clock milliseconds
/// since the key was pressed (the build's own stages come from <see cref="I76.Game.World.MissionBuilder.Trace"/>), to see what the switch spends its time on.
/// </summary>
public static class SwitchTimer
{
    public static readonly bool On = OS.GetEnvironment("I76_SWITCH_TIMING") is { Length: > 0 };

    /// <summary>I76_SWITCH_MEM=1: the memory lines collect the managed heap first, to tell what the collector can free from what is still held.</summary>
    private static readonly bool Collect = OS.GetEnvironment("I76_SWITCH_MEM") is { Length: > 0 };

    public static void Mark(string what, bool memory = false)
    {
        if (!On || EditorSession.SwitchedAtMs == 0) return;
        string mem = memory ? ", " + Memory() : "";
        GD.Print($"switch: {what} @{Time.GetTicksMsec() - EditorSession.SwitchedAtMs} ms{mem}");
    }

    /// <summary>The process's memory in one line: resident set, the engine's, the renderer's, object counts and the managed heap (collected first with I76_SWITCH_MEM=1).</summary>
    public static string Memory()
    {
        if (Collect) { System.GC.Collect(); System.GC.WaitForPendingFinalizers(); }
        long M(Performance.Monitor m) => (long)Performance.GetMonitor(m);
        return $"resident {Resident() >> 20} MB ({Split()}), engine {M(Performance.Monitor.MemoryStatic) >> 20} MB, video {M(Performance.Monitor.RenderVideoMemUsed) >> 20} MB (textures {M(Performance.Monitor.RenderTextureMemUsed) >> 20}, buffers {M(Performance.Monitor.RenderBufferMemUsed) >> 20}), {M(Performance.Monitor.ObjectNodeCount)} nodes ({M(Performance.Monitor.ObjectOrphanNodeCount)} orphans), {M(Performance.Monitor.ObjectResourceCount)} resources, {M(Performance.Monitor.ObjectCount)} objects, managed {System.GC.GetTotalMemory(false) >> 20} MB";
    }

    /// <summary>What the resident set is made of (Linux): anonymous memory (the allocators' heaps), file mappings, shared memory (the GPU driver's).</summary>
    private static string Split()
    {
        try
        {
            long Of(string key) { foreach (var l in System.IO.File.ReadLines("/proc/self/status")) if (l.StartsWith(key)) return long.Parse(l.Split(':')[1].Trim().Split(' ')[0]) >> 10; return 0; }
            return $"anon {Of("RssAnon")}, file {Of("RssFile")}, shmem {Of("RssShmem")}, " + I76.Game.Boot.HeapTrim.Describe();
        }
        catch (System.Exception) { return "-"; }
    }

    /// <summary>The process's resident set, bytes (Linux; 0 elsewhere).</summary>
    private static long Resident()
    {
        try { return long.Parse(System.IO.File.ReadAllText("/proc/self/statm").Split(' ')[1]) * 4096; }
        catch (System.Exception) { return 0; }
    }
}
