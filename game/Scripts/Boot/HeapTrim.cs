using System.Runtime.InteropServices;

namespace I76.Game.Boot;

/// <summary>
/// Gives the C allocator's free pages back to the system. A scene freed after a big build (a map's meshes, textures and arrays: hundreds of
/// MB) leaves the memory in the allocator's arenas, still counted against the process, and the next scene's allocations come from fragments
/// of it; trimming after the free keeps the resident set where it was instead of stepping up with every playtest or mission restart.
/// </summary>
public static class HeapTrim
{
    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int MallOpt(int param, int value);
    private const int MmapThreshold = -3;
    private static bool _set;

    /// <summary>
    /// Large blocks (a mesh's arrays, an image, a baked layer) go straight to the system's mappings and come back to it when freed, instead of
    /// the allocator growing its limit for them to its own heap, where what is freed stays as holes between what is not (Linux; once).
    /// </summary>
    public static void Configure()
    {
        if (_set || !System.OperatingSystem.IsLinux() || System.Environment.GetEnvironmentVariable("I76_MALLOC_DEFAULT") == "1") return;
        _set = true;
        try { MallOpt(MmapThreshold, 1 << 20); }
        catch (System.Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallInfo { public nuint Arena, Ordblks, Smblks, Hblks, Hblkhd, Usmblks, Fsmblks, Uordblks, Fordblks, Keepcost; }

    [DllImport("libc", EntryPoint = "mallinfo2")]
    private static extern MallInfo Mallinfo2();

    /// <summary>The C allocator's own count, MB: in use by the program, free in its arenas, and mapped directly (large blocks); for the memory lines.</summary>
    public static string Describe()
    {
        try { var m = Mallinfo2(); return $"malloc used {(long)m.Uordblks >> 20}, free {(long)m.Fordblks >> 20}, mapped {(long)m.Hblkhd >> 20}"; }
        catch (System.Exception) { return "-"; }
    }

    /// <summary>Collects the managed heap and trims the native one (Linux; elsewhere only the collection).</summary>
    public static void Run()
    {
        // twice: freeing a wrapper's native object can release the last reference to another's, which the first pass could not see
        for (int pass = 0; pass < 3; pass++)
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
        }
        if (!System.OperatingSystem.IsLinux()) return;
        try { MallocTrim(0); }
        catch (System.Exception) { }   // no such libc: nothing to give back
    }
}
